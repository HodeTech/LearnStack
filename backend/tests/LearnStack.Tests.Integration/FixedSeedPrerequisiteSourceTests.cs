using System.Data;
using System.Data.Common;
using System.Reflection;
using LearnStack.Application.Authorization;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
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

/// <summary>Real console provenance/catalogue/invocation and production minter; no database qualification is claimed here.</summary>
public sealed class FixedSeedPrerequisiteSourceTests
{
    [Theory]
    [InlineData("role")]
    [InlineData("first-admin")]
    [InlineData("member")]
    [InlineData("profile")]
    [InlineData("organization")]
    [InlineData("host")]
    [InlineData("locale")]
    [InlineData("branding")]
    public async Task The_eight_exact_consumers_bind_the_original_request_plan_owner_and_generation(string act)
    {
        var (request, context, users) = Act(act);
        await using var fixture = new Fixture(context);
        var plan = new IdentityMutationLockPlan(users, context.TenantId, IdentityTenantLockMode.Exclusive);
        fixture.Locks.Held = plan;
        SeedPrerequisiteReadWitness? retained = null;
        await fixture.ExecuteAsync(request, () =>
        {
            retained = fixture.Capture(request, plan);
            Assert.NotNull(retained);
            Assert.Same(request, retained.Invocation.Operation);
            Assert.Same(plan, retained.Plan);
            Assert.Equal(context.TenantId, retained.TenantId);
            Assert.Equal(context.OrganizationId, retained.OrganizationId);
            Assert.Null(fixture.Capture(request, plan));
            Assert.False(retained.TryBindTo(fixture.Owner, fixture.Owner.Transaction, 2));
            Assert.True(retained.TryBindTo(fixture.Owner, fixture.Owner.Transaction, 1));
            Assert.False(retained.TryBindTo(fixture.Owner, fixture.Owner.Transaction, 1));
            retained.RequireCurrent();
            var declaration = retained.GetDeclaration<FixedSeedPrerequisiteDeclaration>();
            Assert.Equal(8, declaration.Roles.Length);
            Assert.Equal(2, declaration.Memberships.Length);
            Assert.Equal(IdentityFoundationManifest.Version1.Fingerprint, declaration.ManifestFingerprint);
        });
        Assert.NotNull(retained);
        Assert.False(retained.IsActive);
    }

    [Theory]
    [InlineData("request-copy")]
    [InlineData("changed-value")]
    [InlineData("shared-plan")]
    [InlineData("missing-user")]
    [InlineData("extra-user")]
    [InlineData("foreign-catalogue")]
    [InlineData("wrong-generation")]
    public async Task Seed_shape_and_system_attribution_do_not_replace_exact_production_pins(string mismatch)
    {
        var (original, context, users) = Act("profile");
        var command = Assert.IsType<SetSeedMembershipProfileCommand>(original);
        object request = mismatch == "changed-value" ? command with { DisplayName = "Changed" } : command;
        await using var fixture = new Fixture(context, foreignCatalogue: mismatch == "foreign-catalogue");
        if (mismatch == "wrong-generation") fixture.Owner.Generation = 0;
        var plan = new IdentityMutationLockPlan(mismatch switch
        {
            "missing-user" => [],
            "extra-user" => [.. users, UserId.From(Guid.CreateVersion7())],
            _ => users,
        }, context.TenantId, mismatch == "shared-plan" ? IdentityTenantLockMode.Shared : IdentityTenantLockMode.Exclusive);
        fixture.Locks.Held = plan;
        await fixture.ExecuteAsync(request, () => Assert.Null(fixture.Capture(
            mismatch == "request-copy" ? command with { } : request, plan)));
        Assert.Equal(0, fixture.Locks.Acquisitions);
    }

    [Fact]
    public async Task A_lost_exact_plan_or_disposed_console_source_cannot_leave_a_usable_witness()
    {
        var (request, context, users) = Act("profile");
        await using var fixture = new Fixture(context);
        var plan = new IdentityMutationLockPlan(users, context.TenantId, IdentityTenantLockMode.Exclusive);
        fixture.Locks.Held = plan;
        await fixture.ExecuteAsync(request, () =>
        {
            var witness = fixture.Capture(request, plan)!;
            fixture.Locks.Held = new IdentityMutationLockPlan(users, context.TenantId, IdentityTenantLockMode.Exclusive);
            Assert.Throws<IdentityMutationLockException>(witness.RequireCurrent);
            fixture.Locks.Held = plan;
            fixture.DisposeSource();
            Assert.False(witness.IsActive);
            Assert.Throws<InvalidOperationException>(witness.RequireCurrent);
        });
    }

    [Fact]
    public async Task Provisioning_keeps_no_temporary_prerequisite_authority()
    {
        var tenant = SeedData.English;
        await using var fixture = new Fixture(new SeedIdentityTenantContext(tenant.TenantId, null));
        var request = new ProvisionTenantCommand(tenant.TenantId, tenant.Slug, tenant.DisplayName,
            tenant.DefaultOrganization.OrganizationId, tenant.DefaultOrganization.Slug, tenant.DefaultOrganization.DisplayName);
        var plan = new IdentityMutationLockPlan([], tenant.TenantId, IdentityTenantLockMode.Exclusive);
        fixture.Locks.Held = plan;
        await fixture.ExecuteAsync(request, () => Assert.Null(fixture.Capture(request, plan)));
    }

    private static (object Request, SeedIdentityTenantContext Context, UserId[] Users) Act(string act)
    {
        var tenant = SeedData.Yoga;
        var manifest = IdentityFoundationManifest.Version1;
        var member = SeedData.IdentityMemberships.Single(value => value.Identity.Tenant == tenant
            && value.IsFirstAdmin == (act == "first-admin"));
        var role = SeedData.IdentityRoles.First(value => value.Tenant == tenant);
        var context = new SeedIdentityTenantContext(tenant.TenantId, act is "member" or "profile" ? member.OrganizationId : null);
        object request = act switch
        {
            "role" => new InitializeSeedRoleCommand(role.RoleId, role.BuiltInKey, role.Scope, manifest.Version,
                manifest.RoleFingerprint(tenant.TenantId, role.RoleId, role.BuiltInKey, role.Scope)),
            "first-admin" => new CreateFirstSeedAdminMembershipCommand(member.MembershipId, member.Identity.UserId,
                member.Role.RoleId, manifest.Version, manifest.MembershipFingerprint(tenant.TenantId, member.OrganizationId,
                    member.MembershipId, member.Identity.UserId, member.Role.RoleId)),
            "member" => new CreateSeedMembershipCommand(member.MembershipId, member.Identity.UserId, member.Role.RoleId, member.Scope,
                manifest.Version, manifest.MembershipFingerprint(tenant.TenantId, member.OrganizationId, member.MembershipId,
                    member.Identity.UserId, member.Role.RoleId)),
            "profile" => new SetSeedMembershipProfileCommand(member.MembershipId, member.Identity.UserId, member.Role.RoleId,
                member.Scope, member.DisplayName, member.PreferredLocale, manifest.Version,
                manifest.ProfileFingerprint(tenant.TenantId, member.OrganizationId, member.MembershipId, member.DisplayName, member.PreferredLocale)),
            "organization" => new CreateOrganizationCommand(tenant.SecondOrganization.OrganizationId,
                tenant.SecondOrganization.Slug, tenant.SecondOrganization.DisplayName),
            "host" => new MapHostToTenantCommand(tenant.Host, tenant.DefaultOrganization.OrganizationId, true, true),
            "locale" => new AddTenantLocaleCommand(1, "tr-TR", true, true, 0),
            "branding" => new SetTenantBrandingCommand(tenant.Curriculum!.Theme.Id, tenant.Curriculum.Theme.Value, null),
            _ => throw new ArgumentOutOfRangeException(nameof(act)),
        };
        return (request, context, act is "first-admin" or "member" or "profile" ? [member.Identity.UserId] : []);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _data;
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;
        private readonly object _source;
        private readonly MethodInfo _capture;
        private readonly OperationAuthorizationCoordinator _coordinator;

        internal Fixture(SeedIdentityTenantContext context, bool foreignCatalogue = false)
        {
            _data = NpgsqlDataSource.Create("Host=localhost;Database=fixed_seed_source_no_io;Username=learnstack_app");
            _provider = SeedComposition.Build(_data, context, NullLoggerFactory.Instance);
            _scope = _provider.CreateAsyncScope();
            var services = _scope.ServiceProvider;
            _coordinator = services.GetRequiredService<OperationAuthorizationCoordinator>();
            Owner = new(context);
            var catalogue = foreignCatalogue
                ? new OperationPolicyCatalog([new IdentityOperationPolicySource(), new TenancyOperationPolicySource()])
                : services.GetRequiredService<IOperationPolicyCatalog>();
            // Exercise the internal production minter without adding a test-only public constructor or source.
            var type = typeof(SeedComposition).Assembly.GetType("LearnStack.Tools.Seeder.FixedSeedPrerequisiteSource", throwOnError: true)!;
            _source = Activator.CreateInstance(type, _coordinator, catalogue, context, Owner, Locks,
                services.GetServices<ITrustedOperationSource>())!;
            _capture = type.GetMethod("Capture", BindingFlags.Instance | BindingFlags.NonPublic)!;
        }
        internal SourceOwner Owner { get; }
        internal HeldLocks Locks { get; } = new();
        internal SeedPrerequisiteReadWitness? Capture(object request, IdentityMutationLockPlan plan) =>
            (SeedPrerequisiteReadWitness?)_capture.Invoke(_source, [request, plan]);
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
            throw new InvalidOperationException("A prerequisite source cannot acquire another plan.");
        }
        public void RequireCurrent(IdentityMutationLockPlan plan)
        {
            if (!ReferenceEquals(plan, Held)) throw new IdentityMutationLockException();
        }
    }

    private sealed class SourceOwner(ITenantContext context) : IUnitOfWork
    {
        internal long Generation { get; set; } = 1;
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
        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class SourceTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;
        public override void Commit() => throw new NotSupportedException();
        public override void Rollback() => throw new NotSupportedException();
    }
}
