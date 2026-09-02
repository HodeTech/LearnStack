using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The guard that turns an unannounced transaction from a silence into a failure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Connected as <c>learnstack_app</c>.</b> The whole subject is what a request-path
/// connection does when nobody announced its tenant, and a bypass role would answer a
/// different question.
/// </para>
/// <para>
/// <b>These are diagnostic tests, not isolation tests.</b> Row Level Security already
/// makes the unannounced state safe — the first case below proves exactly that, and it
/// is the reason the guard is worth having: safe is not the same as visible, and an
/// empty result set arriving from production gets investigated as a bug in the feature.
/// Removing the interceptor removes a diagnostic, never a protection, and no assertion
/// here should be read as covering isolation.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class TenantContextGuardTests
{
    private readonly SchemaFixture _schema;

    public TenantContextGuardTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task Without_The_Guard_An_Unannounced_Read_Is_Silent_And_Empty()
    {
        // The state the guard exists for, observed directly rather than described.
        // Issued as a raw command, which EF interception cannot see, so this is what the
        // application would have got back: rows the tenant owns, filtered to nothing by a
        // NULL predicate, with no error anywhere. Safe, and indistinguishable from a
        // tenant that simply has no organizations.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

        await using var read = new NpgsqlCommand(
            "SELECT count(*) FROM organizations", connection, transaction);

        (await read.ExecuteScalarAsync(CancellationToken.None)).Should().Be(0L,
            "with app.tenant_id unset every policy predicate is NULL — fail-closed, and "
            + "the failure mode is an empty result set rather than an error");
    }

    [Fact]
    public async Task An_Announced_Transaction_Is_Let_Through()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await AnnounceAsync(scope.ServiceProvider, unitOfWork, SchemaFixture.TenantA);

        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        (await context.Organizations.CountAsync()).Should().BeGreaterThan(0,
            "the announcement is what makes the policy admit the tenant's own rows");

        await unitOfWork.RollbackAsync();
    }

    [Fact]
    public async Task An_Unannounced_Read_Throws_Instead_Of_Returning_Nothing()
    {
        // The same query as above, on a transaction nobody announced. Without the
        // interceptor this returns zero rows and says nothing.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();

        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var act = async () => await context.Organizations.CountAsync();

        (await act.Should().ThrowAsync<TenantContextMissingException>())
            .Which.Message.Should().Contain("SetTenantContextAsync",
                "the message names the announcement a reader has to go and find");

        await unitOfWork.RollbackAsync();
    }

    [Fact]
    public async Task An_Unannounced_Write_Throws_Too()
    {
        // EF routes a SaveChanges INSERT through ReaderExecuting rather than
        // NonQueryExecuting, so a guard covering only the non-query arms would let every
        // write past. Asserted separately from the read for that reason.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();

        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var act = async () => await context.Database.ExecuteSqlRawAsync(
            "UPDATE organizations SET slug = slug WHERE false");

        await act.Should().ThrowAsync<TenantContextMissingException>();

        await unitOfWork.RollbackAsync();
    }

    [Fact]
    public async Task A_Nested_Frame_Inherits_The_Announcement()
    {
        // SetTenantContextAsync returns early for a joiner — re-issuing would let an
        // inner frame retarget the outer frame's tenant. So the mark has to belong to the
        // transaction, not to the frame, or every nested handler would trip the guard.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await AnnounceAsync(scope.ServiceProvider, unitOfWork, SchemaFixture.TenantA);

        await using (await unitOfWork.BeginTransactionAsync())
        {
            var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var act = async () => await context.Organizations.CountAsync();

            await act.Should().NotThrowAsync();
        }

        await unitOfWork.RollbackAsync();
    }

    [Fact]
    public async Task A_Second_Transaction_Does_Not_Inherit_The_First_Ones_Announcement()
    {
        // The mark is per physical transaction. Measured on Npgsql 10, a pooled data
        // source hands back the SAME NpgsqlTransaction instance across sequential
        // cycles — so anything keyed on the transaction object, or a flag nobody cleared
        // at BEGIN, would vouch for this second transaction on the strength of the
        // first's announcement. That is the bug class the unit of work's own generation
        // counter already records shipping once.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // Committed rather than rolled back: a rollback marks the unit rollback-only for
        // its life, so a second transaction on it is refused outright and this scenario
        // would be unreachable. A commit is the path the unit's own reset comment
        // describes — "a unit that committed and then opened a second transaction".
        await using (await unitOfWork.BeginTransactionAsync())
        {
            await AnnounceAsync(scope.ServiceProvider, unitOfWork, SchemaFixture.TenantA);
            await unitOfWork.CommitAsync();
        }

        await unitOfWork.BeginTransactionAsync();

        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var act = async () => await context.Organizations.CountAsync();

        await act.Should().ThrowAsync<TenantContextMissingException>(
            "a new transaction is unannounced until someone announces it");

        await unitOfWork.RollbackAsync();
    }

    [Fact]
    public async Task A_Synchronous_Read_Is_Guarded_Too()
    {
        // EF does not route the synchronous APIs through the asynchronous ones, so the
        // six overrides are three independent pairs. Measured: commenting the guard out
        // of the three synchronous arms left every other case here green, because they
        // all await. A caller using the blocking API would have walked straight past.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();

        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

#pragma warning disable xUnit1031 // The blocking API is the subject, not an accident.
        var read = () => context.Organizations.Count();
        var write = () => context.Database.ExecuteSqlRaw("UPDATE organizations SET slug = slug WHERE false");
#pragma warning restore xUnit1031

        read.Should().Throw<TenantContextMissingException>();
        write.Should().Throw<TenantContextMissingException>();

        await unitOfWork.RollbackAsync();
    }

    [Fact]
    public async Task An_Announcement_Vouches_For_Its_Own_Transaction_And_No_Other()
    {
        // The identity half of the check, which the reset at BEGIN hides in every
        // sequential case — measured, dropping ReferenceEquals left the whole suite
        // green. It matters because Npgsql recycles transaction objects: a pooled data
        // source hands back the same NpgsqlTransaction instance across cycles, so a flag
        // read without comparing against the unit's OWN live transaction would vouch for
        // whatever came next.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await AnnounceAsync(scope.ServiceProvider, unitOfWork, SchemaFixture.TenantA);

        unitOfWork.IsTenantContextIssuedOn(unitOfWork.Transaction).Should().BeTrue();
        unitOfWork.IsTenantContextIssuedOn(null).Should().BeFalse(
            "a command outside any transaction is announced by nothing");

        // Some other transaction, on a connection this unit does not own.
        await using var elsewhere = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        await using var otherConnection = await elsewhere.OpenConnectionAsync(CancellationToken.None);
        await using var otherTransaction =
            await otherConnection.BeginTransactionAsync(CancellationToken.None);

        unitOfWork.IsTenantContextIssuedOn(otherTransaction).Should().BeFalse(
            "the announcement is about one transaction, not about the unit's mood");

        await unitOfWork.RollbackAsync();
    }

    private static async Task AnnounceAsync(
        IServiceProvider scope, IUnitOfWork unitOfWork, Guid tenant)
    {
        var context = new AnnouncedContext(tenant);
        scope.GetRequiredService<ITenantContextAccessor>().Current = context;
        await unitOfWork.SetTenantContextAsync(context);
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString));
        services.AddLogging();
        services.AddSingleton<ITenantContextAccessor, FlowingAccessor>();
        services.AddTransient<ITenantContext>(sp =>
            sp.GetRequiredService<ITenantContextAccessor>().Current
            ?? UnresolvedTenantContext.Instance);
        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddModuleDbContext<TenancyDbContext>();
        return services.BuildServiceProvider();
    }

    private sealed class FlowingAccessor : ITenantContextAccessor
    {
        public ITenantContext? Current { get; set; }
    }

    private sealed class AnnouncedContext(Guid tenant) : ITenantContext
    {
        public bool IsResolved => true;

        public TenantId TenantId { get; } = TenantId.From(tenant);

        public OrganizationId? OrganizationId => null;

        public UserId? UserId => null;

        public TenantContextOrigin? Origin => TenantContextOrigin.HostAndClaim;

        public string? CorrelationId => null;

        public string? ModuleName => null;
    }
}
