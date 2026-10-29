using System.Data;
using System.Data.Common;
using System.Reflection;
using LearnStack.Application.Authorization;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>Actual console minter/catalogue/source controls without any database I/O.</summary>
public sealed class FixedSeedCompletionSourceTests
{
    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(false, 0, true)]
    [InlineData(false, 1, true)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, true)]
    public async Task Only_the_fixed_prepared_origin_invocation_gets_its_separate_one_use_completion(
        bool yoga, int marker, bool existingMapping)
    {
        var tenant = yoga ? SeedData.Yoga : SeedData.English;
        await using var fixture = new Fixture(tenant);
        var command = new AdoptSeedAdministrationOriginCommand(4);
        var target = Target(tenant, marker, existingMapping);
        var plan = new IdentityMutationLockPlan([], tenant.TenantId, IdentityTenantLockMode.Exclusive);
        fixture.Locks.Held = plan;
        SeedCompletionReadWitness? retained = null;
        await fixture.ExecuteAsync(command, () =>
        {
            retained = fixture.Capture(command, target, plan);
            Assert.NotNull(retained);
            Assert.Same(command, retained.Invocation.Operation);
            Assert.Same(target, retained.GetPreparedTarget<SeedAdministrationOriginPreparedTarget>());
            Assert.Equal(tenant.TenantId, retained.GetDeclaration<SeedTenantDeclaration>().TenantId);
            Assert.Equal(tenant.TenantId, retained.GetDeclaration<SeedCustomizationDeclaration>().TenantId);
            Assert.Equal(tenant.TenantId, retained.GetDeclaration<SeedEducationDeclaration>().TenantId);
            Assert.Equal(8, retained.GetDeclaration<SeedIdentityCompletionDeclaration>().Roles.Length);
            Assert.True(retained.IsExpectedOn(fixture.Owner, fixture.Owner.Transaction, 1));
            Assert.False(retained.IsExpectedOn(fixture.Owner, fixture.Owner.Transaction, 2));
            Assert.True(retained.IsExpectedOn(fixture.Owner, fixture.Owner.Transaction, 1));
            Assert.False(retained.TryBindTo(fixture.Owner, fixture.Owner.Transaction, 2));
            Assert.True(retained.TryBindTo(fixture.Owner, fixture.Owner.Transaction, 1));
            Assert.False(retained.TryBindTo(fixture.Owner, fixture.Owner.Transaction, 1));
            Assert.Null(fixture.Capture(command, target, plan));
            retained.RequireCurrent();
        });
        Assert.NotNull(retained);
        Assert.False(retained.IsActive);
        Assert.Equal(0, fixture.Locks.Acquisitions);
    }

    [Theory]
    [InlineData("request-copy")]
    [InlineData("stale-target")]
    [InlineData("completed-marker")]
    [InlineData("legacy-without-mapping")]
    [InlineData("foreign-target")]
    [InlineData("foreign-mapping")]
    [InlineData("branch-mapping")]
    [InlineData("disabled-mapping")]
    [InlineData("wrong-host")]
    [InlineData("shared-plan")]
    [InlineData("extra-user")]
    [InlineData("foreign-catalogue")]
    [InlineData("wrong-generation")]
    [InlineData("wide-owner")]
    public async Task Caller_values_or_seed_shape_do_not_replace_actual_preparation_and_console_provenance(string mismatch)
    {
        var tenant = SeedData.English;
        await using var fixture = new Fixture(tenant, foreignCatalogue: mismatch == "foreign-catalogue");
        if (mismatch == "wrong-generation") fixture.Owner.Generation = 0;
        if (mismatch == "wide-owner") fixture.Owner.IsTenantWideRead = true;
        var command = new AdoptSeedAdministrationOriginCommand(4);
        var declared = SeedAdministrationOriginManifest.Find(tenant.TenantId)!;
        var target = new SeedAdministrationOriginPreparedTarget(
            mismatch == "foreign-target" ? SeedData.Yoga.TenantId : tenant.TenantId, tenant.Slug,
            mismatch == "stale-target" ? 5 : 4, mismatch == "completed-marker" ? 2 : mismatch == "legacy-without-mapping" ? 1 : 0,
            tenant.DefaultOrganization.OrganizationId, mismatch == "legacy-without-mapping" ? null
                : new SeedAdministrationOriginPreparedMapping(mismatch == "wrong-host" ? "unlisted.example.test" : declared.Host,
                    mismatch == "foreign-mapping" ? SeedData.Yoga.TenantId : tenant.TenantId,
                    mismatch == "branch-mapping" ? tenant.DefaultOrganization.OrganizationId : null,
                    mismatch != "disabled-mapping", true));
        var plan = new IdentityMutationLockPlan(mismatch == "extra-user" ? [SeedData.IdentityUsers[0].UserId] : [],
            tenant.TenantId, mismatch == "shared-plan" ? IdentityTenantLockMode.Shared : IdentityTenantLockMode.Exclusive);
        fixture.Locks.Held = plan;
        await fixture.ExecuteAsync(command, () => Assert.Null(fixture.Capture(
            mismatch == "request-copy" ? command with { } : command, target, plan)));
        Assert.Equal(0, fixture.Locks.Acquisitions);
    }

    [Fact]
    public async Task A_pristine_writer_has_no_completion_authority_and_lifetime_or_plan_loss_revokes_a_retained_witness()
    {
        var tenant = SeedData.English;
        var plan = new IdentityMutationLockPlan([], tenant.TenantId, IdentityTenantLockMode.Exclusive);
        await using (var pristine = new Fixture(tenant))
        {
            pristine.Locks.Held = plan;
            var other = new CreateOrganizationCommand(tenant.SecondOrganization.OrganizationId,
                tenant.SecondOrganization.Slug, tenant.SecondOrganization.DisplayName);
            await pristine.ExecuteAsync(other, () => Assert.Null(pristine.Capture(new(4), Target(tenant, 0, false), plan)));
        }
        await using var fixture = new Fixture(tenant);
        fixture.Locks.Held = plan;
        var command = new AdoptSeedAdministrationOriginCommand(4);
        await fixture.ExecuteAsync(command, () =>
        {
            var witness = fixture.Capture(command, Target(tenant, 0, false), plan)!;
            fixture.Locks.Held = new IdentityMutationLockPlan([], tenant.TenantId, IdentityTenantLockMode.Exclusive);
            Assert.Throws<IdentityMutationLockException>(witness.RequireCurrent);
            fixture.Locks.Held = plan;
            fixture.DisposeSource();
            Assert.False(witness.IsActive);
            Assert.Throws<InvalidOperationException>(witness.RequireCurrent);
        });
    }

    private static SeedAdministrationOriginPreparedTarget Target(SeedTenant tenant, int marker, bool mapping) =>
        new(tenant.TenantId, tenant.Slug, 4, marker, tenant.DefaultOrganization.OrganizationId,
            mapping ? new(SeedAdministrationOriginManifest.Find(tenant.TenantId)!.Host, tenant.TenantId, null, true, true) : null);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _data;
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;
        private readonly object _source;
        private readonly MethodInfo _capture;
        private readonly OperationAuthorizationCoordinator _coordinator;

        internal Fixture(SeedTenant tenant, bool foreignCatalogue = false)
        {
            var context = new SeedTenantContext(tenant.TenantId, null);
            _data = NpgsqlDataSource.Create("Host=localhost;Database=fixed_completion_source_no_io;Username=learnstack_app");
            _provider = SeedComposition.Build(_data, context, NullLoggerFactory.Instance);
            _scope = _provider.CreateAsyncScope();
            var services = _scope.ServiceProvider;
            _coordinator = services.GetRequiredService<OperationAuthorizationCoordinator>();
            Owner = new(context);
            var catalog = foreignCatalogue ? new OperationPolicyCatalog([new TenancyOperationPolicySource()])
                : services.GetRequiredService<IOperationPolicyCatalog>();
            var type = typeof(SeedComposition).Assembly.GetType("LearnStack.Tools.Seeder.FixedSeedCompletionSource", throwOnError: true)!;
            _source = Activator.CreateInstance(type, _coordinator, catalog, context, Owner, Locks,
                services.GetServices<ITrustedOperationSource>())!;
            _capture = type.GetMethod("Capture", BindingFlags.Instance | BindingFlags.NonPublic)!;
        }

        internal SourceOwner Owner { get; }
        internal HeldLocks Locks { get; } = new();
        internal SeedCompletionReadWitness? Capture(AdoptSeedAdministrationOriginCommand command,
            SeedAdministrationOriginPreparedTarget target, IdentityMutationLockPlan plan) =>
            (SeedCompletionReadWitness?)_capture.Invoke(_source, [command, target, plan]);
        internal async Task ExecuteAsync(object request, Action body)
        {
            var result = await _coordinator.ExecuteRequestAsync<object, Result<None>>(request, () =>
            {
                body();
                return Task.FromResult(Result.Ok(None.Value));
            });
            Assert.True(result.IsSuccess);
        }
        internal void DisposeSource() => ((IDisposable)_source).Dispose();
        public async ValueTask DisposeAsync()
        {
            DisposeSource();
            await Owner.DisposeAsync();
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
            await _data.DisposeAsync();
        }
    }

    private sealed class HeldLocks : IIdentityMutationLocks
    {
        internal IdentityMutationLockPlan? Held { get; set; }
        internal int Acquisitions { get; private set; }
        public Task AcquireAsync(IdentityMutationLockPlan plan, CancellationToken cancellationToken = default)
        {
            Acquisitions++;
            throw new InvalidOperationException("A completion source cannot acquire another plan.");
        }
        public void RequireCurrent(IdentityMutationLockPlan plan)
        {
            if (!ReferenceEquals(plan, Held)) throw new IdentityMutationLockException();
        }
    }

    private sealed class SourceOwner(ITenantContext context) : IUnitOfWork, IOperationReadScopeView
    {
        internal long Generation { get; set; } = 1;
        public bool IsTenantWideRead { get; set; }
        public DbConnection Connection { get; } = new NpgsqlConnection();
        public DbTransaction Transaction { get; } = new SourceTransaction();
        public bool HasActiveTransaction => true;
        public TransactionMode? Mode => TransactionMode.ReadWrite;
        public bool IsRollbackOnly { get; private set; }
        public void MarkRollbackOnly() => IsRollbackOnly = true;
        public TenantContextAnnouncement? GetTenantContextAnnouncement(DbTransaction? transaction) =>
            ReferenceEquals(transaction, Transaction) ? new(Generation, context.TenantId, context.OrganizationId) : null;
        public bool IsTenantContextIssuedOn(DbTransaction? transaction) => ReferenceEquals(transaction, Transaction);
        public Task<IUnitOfWorkScope> BeginTransactionAsync(TransactionMode mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IUnitOfWorkScope> BeginTransactionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetTenantContextAsync(ITenantContext value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetProvisioningTenantContextAsync(TenantId tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CommitAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RollbackAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async ValueTask DisposeAsync() { await Transaction.DisposeAsync(); await Connection.DisposeAsync(); }
    }

    private sealed class SourceTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;
        public override void Commit() => throw new NotSupportedException();
        public override void Rollback() => throw new NotSupportedException();
    }
}
