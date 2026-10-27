using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Infrastructure.Authorization;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Actual contextual authority and Tenancy lifecycle rows, isolated from shared schema/persona fixtures.</summary>
public sealed class IdentityAuthorizationReaderFixture : IAsyncLifetime
{
    public IdentityMembershipFixture Membership { get; } = new();
    public string AppConnectionString => Membership.Postgres.AppConnectionString;

    public async Task InitializeAsync()
    {
        await Membership.InitializeAsync();
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        foreach (var tenant in new[] { SchemaFixture.TenantA, SchemaFixture.TenantB })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await ScopeAsync(app, transaction, tenant);
            await ExecuteAsync(app, transaction, """
                INSERT INTO public.tenants(id,slug,display_name,status,created_at,created_by,row_version)
                VALUES(@tenant,@slug,'Authorization proof','Trial',clock_timestamp(),@actor,0)
                """, ("tenant", tenant), ("slug", "authorization-" + tenant.ToString("N")), ("actor", UserId.SystemActor.Value));
            var organizations = tenant == SchemaFixture.TenantA
                ? new[] { SchemaFixture.OrgA1, SchemaFixture.OrgA2 } : [SchemaFixture.OrgB1];
            foreach (var organization in organizations)
                await ExecuteAsync(app, transaction, """
                    INSERT INTO public.organizations(id,tenant_id,slug,display_name,status,created_at,created_by,row_version)
                    VALUES(@organization,@tenant,@slug,'Authorization branch','Active',clock_timestamp(),@actor,0)
                    """, ("organization", organization), ("tenant", tenant), ("slug", "branch-" + organization.ToString("N")),
                    ("actor", UserId.SystemActor.Value));
            await transaction.CommitAsync();
        }
    }

    public Task DisposeAsync() => Membership.DisposeAsync();

    public async Task<(Guid Actor, Guid Membership)> AddMemberAsync(Guid? organization = null,
        BuiltInRoleKey roleKey = BuiltInRoleKey.Learner)
    {
        var actor = Guid.CreateVersion7();
        var member = Guid.CreateVersion7();
        await InsertUserAsync(AppConnectionString, actor);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA, organization);
        await LockAsync(app, transaction, SchemaFixture.TenantA, actor, false);
        await InsertMemberAsync(app, transaction, SchemaFixture.TenantA, organization, member, actor,
            RoleId(SchemaFixture.TenantA, roleKey, organization is null ? PermissionScope.Tenant : PermissionScope.Organization));
        await transaction.CommitAsync();
        return (actor, member);
    }
}

internal sealed class AuthorizationReaderHarness : IAsyncDisposable
{
    private readonly NpgsqlDataSource _business;
    private readonly NpgsqlUnitOfWork _ambient;

    private AuthorizationReaderHarness(IdentityAuthorizationReadSource source, NpgsqlDataSource business,
        NpgsqlUnitOfWork ambient, IIdentityScopeLifecycleReader lifecycle)
    {
        Source = source;
        _business = business;
        _ambient = ambient;
        Lifecycle = lifecycle;
        Reader = new(new PreContextIdentityAuthorizationSessionFactory(source), lifecycle);
    }

    public IdentityAuthorizationReadSource Source { get; }
    public IIdentityScopeLifecycleReader Lifecycle { get; }
    public CommittedIdentityAuthorizationReader Reader { get; }

    public static async Task<AuthorizationReaderHarness> CreateAsync(IdentityAuthorizationReaderFixture fixture,
        Func<IIdentityScopeLifecycleReader, IIdentityScopeLifecycleReader>? decorate = null)
    {
        var source = new IdentityAuthorizationReadSource(fixture.AppConnectionString);
        await source.QualifyAsync(Authority.AuthorityId, Authority.Issuer);
        var business = ApplicationDataSource.Build(fixture.AppConnectionString);
        var ambient = new NpgsqlUnitOfWork(business, NullLogger<NpgsqlUnitOfWork>.Instance);
        var lifecycle = new IdentityScopeLifecycleReader(ambient);
        return new(source, business, ambient, decorate?.Invoke(lifecycle) ?? lifecycle);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Source.DisposeAsync(); }
        finally
        {
            await _ambient.DisposeAsync();
            await _business.DisposeAsync();
        }
    }
}
